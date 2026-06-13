<#
.SYNOPSIS
  Build a signed MSIX for FluentGpu Gallery - a packaged Win32 FULL-TRUST desktop app (NativeAOT, no WindowsAppSDK).

.DESCRIPTION
  Pipeline:  dotnet publish (NativeAOT) -> stage layout -> makepri -> makeappx pack -> signtool sign -> .msix
  This is the improved FluentGpu analogue of WaveeMusic's WinUI/MSBuild packaging - but FluentGpu AOTs cleanly, so the
  package is a single ~12 MB native exe with NO bundled .NET runtime (WaveeMusic ships ~200 MB self-contained JIT).

.EXAMPLE
  pwsh build/pack-msix.ps1                       # host arch, self-signed dev cert, version 0.1.0.0
  pwsh build/pack-msix.ps1 -Arch x64 -Version 0.2.0.0
  pwsh build/pack-msix.ps1 -Install             # build, sign, trust the dev cert, and Add-AppxPackage
  pwsh build/pack-msix.ps1 -NoSign              # leave unsigned (CI re-signs with Trusted Signing)
  pwsh build/pack-msix.ps1 -NoAot               # framework-dependent self-contained (fast iteration; not for release)
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
if (-not $Metadata) { $Metadata = Join-Path $buildDir 'signing\metadata.json' }
$root     = Split-Path -Parent $buildDir
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

# manifest with substituted identity
$mf = (Get-Content $manifestTemplate -Raw).Replace('__PUBLISHER__',$Publisher).Replace('__VERSION__',$Version).Replace('__ARCH__',$Arch)
Set-Content -Path (Join-Path $layout 'AppxManifest.xml') -Value $mf -Encoding UTF8

# 3. makepri - index the scaled/localized resources (the .scale-200 logos resolve via resources.pri)
Step "Generating resources.pri (makepri)"
$priConfig = Join-Path $work 'priconfig.xml'
& $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o | Out-Null
Push-Location $layout
try { & $makepri new /pr $layout /cf $priConfig /of (Join-Path $layout 'resources.pri') /o | Out-Null }
finally { Pop-Location }
if ($LASTEXITCODE -ne 0) { throw "makepri failed ($LASTEXITCODE)." }

# 4. makeappx pack
Step "Packing $outMsix (makeappx)"
Remove-Item $outMsix -Force -ErrorAction SilentlyContinue
& $makeappx pack /o /d $layout /p $outMsix | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx pack failed ($LASTEXITCODE)." }

# 5. sign: Azure Trusted Signing (publicly trusted) OR a self-signed dev cert. CI passes -NoSign and re-signs.
if (-not $NoSign) {
 if ($TrustedSigning) {
  Step "Signing with Azure Trusted Signing"
  if (-not (Test-Path $Metadata)) { throw "Trusted Signing metadata not found: $Metadata  (copy build/signing/metadata.template.json -> metadata.json)." }
  $dlib = @(
    "$env:LOCALAPPDATA\Microsoft\MicrosoftArtifactSigningClientTools\Azure.CodeSigning.Dlib.dll",
    'C:\Program Files (x86)\Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll',
    'C:\Program Files\Microsoft\ArtifactSigningClientTools\bin\Azure.CodeSigning.Dlib.dll',
    'C:\Program Files (x86)\Microsoft\TrustedSigningClientTools\bin\Azure.CodeSigning.Dlib.dll'
  ) | Where-Object { Test-Path $_ } | Select-Object -First 1
  if (-not $dlib) { throw "Azure.CodeSigning.Dlib.dll not found. Install: winget install -e --id Microsoft.Azure.ArtifactSigningClientTools" }
  if (-not ($env:AZURE_CLIENT_ID -and $env:AZURE_TENANT_ID -and $env:AZURE_CLIENT_SECRET)) {
    Write-Host "    no AZURE_* SPN env vars - relying on an existing 'az login' session" -ForegroundColor Yellow
    # The signing account ('Wavee') lives in a specific subscription; select it so DefaultAzureCredential's token has access.
    if ($Subscription) { & az account set --subscription $Subscription 2>$null }
  }
  & $signtool sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib $dlib /dmdf $Metadata $outMsix
  if ($LASTEXITCODE -ne 0) { throw "Trusted Signing failed ($LASTEXITCODE). Check az login / AZURE_*, and that Publisher '$Publisher' matches the cert subject." }
  Write-Host "    signed via Azure Trusted Signing (publicly trusted - no cert import needed)" -ForegroundColor Green
  if ($Install) { Step "Installing (Add-AppxPackage)"; Add-AppxPackage -Path $outMsix }
 }
 else {
  Step "Signing with a self-signed dev cert ($Publisher)"
  $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher -and $_.NotAfter -gt (Get-Date) } | Select-Object -First 1
  if (-not $cert) {
    Write-Host "    creating self-signed cert $Publisher (3y)"
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Publisher `
              -KeyUsage DigitalSignature -FriendlyName 'FluentGpu Dev Signing' `
              -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(3) `
              -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
  }
  & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /tr http://timestamp.digicert.com /td SHA256 $outMsix
  if ($LASTEXITCODE -ne 0) { throw "signtool sign failed ($LASTEXITCODE)." }
  # Verify the chain. A self-signed dev cert legitimately fails /pa (chain not rooted in a trusted CA) until it's
  # installed, so this is informational only. NB: do NOT merge native stderr (2>&1) under ErrorActionPreference=Stop -
  # PS 5.1 wraps it in a NativeCommandError that would terminate the script. Discard stderr + soften EAP instead.
  $eap = $ErrorActionPreference; $ErrorActionPreference = 'SilentlyContinue'
  & $signtool verify /pa $outMsix 1>$null 2>$null
  $trusted = ($LASTEXITCODE -eq 0)
  $ErrorActionPreference = $eap
  if ($trusted) { Write-Host "    signature chain verified + trusted" -ForegroundColor Green }
  else { Write-Host "    signed OK - chain NOT yet trusted (expected for a self-signed dev cert; use -Install or trust '$Publisher')" -ForegroundColor Yellow }

  if ($Install) {
    Step "Trusting the dev cert + installing"
    $cerPath = Join-Path $work 'devcert.cer'
    Export-Certificate -Cert $cert -FilePath $cerPath | Out-Null
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
