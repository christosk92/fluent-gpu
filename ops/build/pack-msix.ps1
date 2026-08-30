<#
.SYNOPSIS
  Build a signed MSIX for FluentGpu Gallery - a packaged Win32 FULL-TRUST desktop app (NativeAOT, no WindowsAppSDK).

.DESCRIPTION
  Pipeline:  dotnet publish (NativeAOT) -> stage layout -> makepri -> makeappx pack -> signtool sign -> .msix
  This is the improved FluentGpu analogue of WaveeMusic's WinUI/MSBuild packaging - but FluentGpu AOTs cleanly, so the
  package is a single ~12 MB native exe with NO bundled .NET runtime (WaveeMusic ships ~200 MB self-contained JIT).

.EXAMPLE
  pwsh ops/build/pack-msix.ps1                       # host arch, self-signed dev cert, version 0.1.0.0
  pwsh ops/build/pack-msix.ps1 -Arch x64 -Version 0.2.0.0
  pwsh ops/build/pack-msix.ps1 -Install             # build, sign, trust the dev cert, and Add-AppxPackage
  pwsh ops/build/pack-msix.ps1 -NoSign              # leave unsigned (CI re-signs with Trusted Signing)
  pwsh ops/build/pack-msix.ps1 -NoAot               # framework-dependent self-contained (fast iteration; not for release)
#>
[CmdletBinding()]
param(
  [ValidateSet('arm64','x64')]
  [string]$Arch = $(if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') { 'arm64' } else { 'x64' }),
  [string]$Version = '0.1.0.0',
  [string]$Configuration = 'Release',
  [string]$Publisher = 'CN=MarTeco Dev, O=MarTeco, C=NL',
  [string]$OutputDir = 'artifacts',
  [switch]$NoAot,
  [switch]$NoSign,
  [switch]$Install,
  [switch]$TrustedSigning,                                   # sign with Azure Trusted Signing (publicly trusted)
  [string]$Metadata,                                         # default set in the body (build/signing/metadata.json)
  [string]$Subscription = 'Azure subscription 1'             # the az subscription holding the 'Wavee' signing account
)
$ErrorActionPreference = 'Stop'
if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') { throw "Version must be 4-part numeric (e.g. 0.1.0.0); got '$Version'." }
# Trusted Signing: the manifest Publisher MUST equal the cert profile Subject Name (else SignTool 0x8007000B). Default to
# the wavee-public-trust subject unless the caller overrode -Publisher.
if ($TrustedSigning -and -not $PSBoundParameters.ContainsKey('Publisher')) { $Publisher = 'CN=cproducts, O=cproducts, L=Utrecht, S=Utrecht, C=NL' }

$buildDir = $PSScriptRoot
# Imported here, not just before signing: Invoke-Native (and the tool discovery below) come from this module and
# are used from the resource step onwards.
Import-Module (Join-Path $buildDir 'Wavee.Build.psm1') -Force -DisableNameChecking
if (-not $Metadata) { $Metadata = Join-Path $buildDir 'signing\metadata.json' }
$root     = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$csproj   = Join-Path $root 'src\FluentGpu.WindowsApp\FluentGpu.WindowsApp.csproj'
$iconDir  = Join-Path $root 'src\FluentGpu.WindowsApp\assets\AppIcon'
$manifestTemplate = Join-Path $buildDir 'AppxManifest.xml'
$rid = "win-$Arch"
$stamp   = "FluentGpu.WindowsApp_${Version}_${Arch}"
$work    = Join-Path $root ".msix-build\$Arch"
$pubDir  = Join-Path $work 'publish'
$layout  = Join-Path $work 'layout'
$outRoot = Join-Path $root $OutputDir
$outMsix = Join-Path $outRoot "$stamp.msix"
function Step($m){ Write-Host "==> $m" -ForegroundColor Cyan }

# 0. locate the Windows SDK tools (makeappx / makepri / signtool)
Step "Locating Windows SDK packaging tools"
$kitsBin = 'C:\Program Files (x86)\Windows Kits\10\bin'
$sdkVer = Get-ChildItem $kitsBin -Directory -ErrorAction SilentlyContinue |
          Where-Object { $_.Name -match '^10\.' -and (Test-Path (Join-Path $_.FullName 'x64\makeappx.exe')) } |
          Sort-Object { [version]$_.Name } | Select-Object -Last 1
if (-not $sdkVer) { throw "No Windows SDK with makeappx.exe found under $kitsBin. Install the Windows SDK." }
$toolDir   = Join-Path $sdkVer.FullName 'x64'        # x64 tools run fine on arm64 (emulation); arch-agnostic anyway
$makeappx  = Join-Path $toolDir 'makeappx.exe'
$makepri   = Join-Path $toolDir 'makepri.exe'
$signtool  = Join-Path $toolDir 'signtool.exe'
Write-Host "    SDK $($sdkVer.Name)  ($toolDir)"

# vswhere on PATH so NativeAOT's ILC can find MSVC link.exe
$vsInstaller = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer'
if ((Test-Path "$vsInstaller\vswhere.exe") -and ($env:PATH -notlike "*$vsInstaller*")) { $env:PATH = "$vsInstaller;$env:PATH" }

# 1. publish (NativeAOT is the csproj default; -NoAot falls back to self-contained JIT)
Step "Publishing $rid ($(if($NoAot){'self-contained JIT'}else{'NativeAOT'}))"
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $pubDir,$outRoot | Out-Null
$pubArgs = @($csproj,'-c',$Configuration,'-r',$rid,'-o',$pubDir,'--nologo','-v','m')
if ($NoAot) { $pubArgs += @('-p:PublishAot=false','--self-contained','true') }
& dotnet publish @pubArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

# 2. stage the package layout
Step "Staging package layout"
New-Item -ItemType Directory -Force -Path $layout | Out-Null
Copy-Item "$pubDir\*" $layout -Recurse -Force
Get-ChildItem $layout -Recurse -Include *.pdb | Remove-Item -Force -ErrorAction SilentlyContinue   # no PDBs in the package

# tile logos -> Assets\ (the manifest references Assets\*.png; the window icon stays at assets\AppIcon\appicon.ico)
$assets = Join-Path $layout 'Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null
foreach ($logo in 'StoreLogo','Square44x44Logo','Square71x71Logo','Square150x150Logo','Square310x310Logo','Wide310x150Logo') {
  Copy-Item (Join-Path $iconDir "$logo.png") $assets -Force
  $scaled = Join-Path $iconDir "$logo.scale-200.png"; if (Test-Path $scaled) { Copy-Item $scaled $assets -Force }
}

# manifest with substituted identity. ReadAllText with an explicit UTF-8 decoder, NOT Get-Content -Raw: under
# Windows PowerShell 5.1 Get-Content defaults to the ANSI codepage and mangles any non-ASCII character in the
# template (the Description's em dash). Written back as UTF-8 WITHOUT a BOM, which is what makeappx expects.
$mf = [IO.File]::ReadAllText($manifestTemplate, [Text.Encoding]::UTF8).Replace('__PUBLISHER__',$Publisher).Replace('__VERSION__',$Version).Replace('__ARCH__',$Arch)
$leftover = [regex]::Matches($mf, '__[A-Z0-9_]+__')
if ($leftover.Count -gt 0) {
  $names = (($leftover | ForEach-Object { $_.Value }) | Sort-Object -Unique) -join ', '
  throw "AppxManifest template has unsubstituted placeholders ($names): $manifestTemplate"
}
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $mf, (New-Object System.Text.UTF8Encoding $false))

# 3. makepri - index the scaled/localized resources (the .scale-200 logos resolve via resources.pri)
Step "Generating resources.pri (makepri)"
# Through Invoke-Native: it captures stdout+stderr and judges the exit code, so a tool that merely warns on
# stderr cannot raise a terminating NativeCommandError under $ErrorActionPreference = 'Stop'.
$priConfig = Join-Path $work 'priconfig.xml'
Invoke-Native $makepri @('createconfig','/cf',$priConfig,'/dq','en-US','/pv','10.0.0','/o') | Out-Null
Push-Location $layout
try { Invoke-Native $makepri @('new','/pr',$layout,'/cf',$priConfig,'/of',(Join-Path $layout 'resources.pri'),'/o') | Out-Null }
finally { Pop-Location }

# 4. makeappx pack
Step "Packing $outMsix (makeappx)"
Remove-Item $outMsix -Force -ErrorAction SilentlyContinue
$pack = Invoke-Native $makeappx @('pack','/o','/d',$layout,'/p',$outMsix) -AllowFailure
if ($pack.ExitCode -ne 0) { throw ("makeappx pack failed ($($pack.ExitCode)):`n" + ($pack.Output -join "`n")) }

# 5. sign: Azure Trusted Signing (publicly trusted) OR a self-signed dev cert. CI passes -NoSign and re-signs.
#    Both paths live in ops/build/Wavee.Build.psm1 (imported at the top) so this script and pack-wavee-msix.ps1
#    sign identically.
if (-not $NoSign) {
 if ($TrustedSigning) {
  Step "Signing with Azure Trusted Signing"
  if (-not ($env:AZURE_CLIENT_ID -and $env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_SECRET)) {
    Write-Host "    no AZURE_* SPN env vars - relying on an existing 'az login' session" -ForegroundColor Yellow
  }
  Invoke-TrustedSigning -Path @($outMsix) -Metadata $Metadata -Subscription $Subscription -SignTool $signtool
  Write-Host "    signed via Azure Trusted Signing (publicly trusted - no cert import needed)" -ForegroundColor Green
  if ($Install) { Step "Installing (Add-AppxPackage)"; Add-AppxPackage -Path $outMsix }
 }
 else {
  Step "Signing with a self-signed dev cert ($Publisher)"
  # Creates the cert on first use, signs, and drops the .cer next to the .msix.
  Invoke-DevCertSigning -Path @($outMsix) -Publisher $Publisher -FriendlyName 'FluentGpu Dev Signing' -SignTool $signtool | Out-Null
  $cerPath = [IO.Path]::ChangeExtension($outMsix, '.cer')
  # Chain check is informational here: a self-signed dev cert legitimately fails /pa until its .cer is trusted.
  if (Test-MsixSignature $outMsix $signtool) { Write-Host "    signature chain verified + trusted" -ForegroundColor Green }
  else { Write-Host "    signed OK - chain NOT yet trusted (expected for a self-signed dev cert; use -Install or trust '$Publisher')" -ForegroundColor Yellow }

  if ($Install) {
    Step "Trusting the dev cert + installing"
    try { Import-Certificate -FilePath $cerPath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null }
    catch { Write-Warning "Could not add the cert to LocalMachine\TrustedPeople (run elevated). Sideload may prompt." }
    Add-AppxPackage -Path $outMsix
  }
 }
}

$size = [Math]::Round((Get-Item $outMsix).Length/1MB,1)
Step "Done"
Write-Host "    $outMsix  (${size} MB, $Arch, v$Version$(if($NoSign){', UNSIGNED'}))" -ForegroundColor Green
if (-not $Install -and -not $NoSign) { Write-Host "    Install:  Add-AppxPackage -Path '$outMsix'   (trust the cert first - see -Install)" }
